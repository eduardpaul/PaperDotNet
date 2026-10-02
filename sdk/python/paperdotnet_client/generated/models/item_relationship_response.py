from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

if TYPE_CHECKING:
    from .located_item_response import LocatedItemResponse
    from .relationship_type_data import RelationshipTypeData

@dataclass
class ItemRelationshipResponse(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The directed property
    directed: Optional[bool] = None
    # The id property
    id: Optional[UUID] = None
    # An item resolved by stable identity, with its current location and the caller's relationship access.
    related_item: Optional[LocatedItemResponse] = None
    # The sourceItemId property
    source_item_id: Optional[UUID] = None
    # The targetItemId property
    target_item_id: Optional[UUID] = None
    # The type property
    type: Optional[RelationshipTypeData] = None

    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> ItemRelationshipResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: ItemRelationshipResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return ItemRelationshipResponse()

    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .located_item_response import LocatedItemResponse
        from .relationship_type_data import RelationshipTypeData

        from .located_item_response import LocatedItemResponse
        from .relationship_type_data import RelationshipTypeData

        fields: dict[str, Callable[[Any], None]] = {
            "directed": lambda n : setattr(self, 'directed', n.get_bool_value()),
            "id": lambda n : setattr(self, 'id', n.get_uuid_value()),
            "relatedItem": lambda n : setattr(self, 'related_item', n.get_object_value(LocatedItemResponse)),
            "sourceItemId": lambda n : setattr(self, 'source_item_id', n.get_uuid_value()),
            "targetItemId": lambda n : setattr(self, 'target_item_id', n.get_uuid_value()),
            "type": lambda n : setattr(self, 'type', n.get_object_value(RelationshipTypeData)),
        }
        return fields

    def serialize(self,writer: SerializationWriter) -> None:
        """
        Serializes information the current object
        param writer: Serialization writer to use to serialize this model
        Returns: None
        """
        if writer is None:
            raise TypeError("writer cannot be null.")
        writer.write_bool_value("directed", self.directed)
        writer.write_uuid_value("id", self.id)
        writer.write_object_value("relatedItem", self.related_item)
        writer.write_uuid_value("sourceItemId", self.source_item_id)
        writer.write_uuid_value("targetItemId", self.target_item_id)
        writer.write_object_value("type", self.type)
        writer.write_additional_data_value(self.additional_data)
