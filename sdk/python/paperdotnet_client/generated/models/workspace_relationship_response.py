from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

if TYPE_CHECKING:
    from .json_object import JsonObject
    from .located_item_response import LocatedItemResponse
    from .relationship_type_data import RelationshipTypeData

@dataclass
class WorkspaceRelationshipResponse(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The attributes property
    attributes: Optional[JsonObject] = None
    # The directed property
    directed: Optional[bool] = None
    # The id property
    id: Optional[UUID] = None
    # An item resolved by stable identity, with its current location and the caller's relationship access.
    source_item: Optional[LocatedItemResponse] = None
    # An item resolved by stable identity, with its current location and the caller's relationship access.
    target_item: Optional[LocatedItemResponse] = None
    # The type property
    type: Optional[RelationshipTypeData] = None
    # The version property
    version: Optional[int] = None

    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> WorkspaceRelationshipResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: WorkspaceRelationshipResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return WorkspaceRelationshipResponse()

    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .json_object import JsonObject
        from .located_item_response import LocatedItemResponse
        from .relationship_type_data import RelationshipTypeData

        from .json_object import JsonObject
        from .located_item_response import LocatedItemResponse
        from .relationship_type_data import RelationshipTypeData

        fields: dict[str, Callable[[Any], None]] = {
            "attributes": lambda n : setattr(self, 'attributes', n.get_object_value(JsonObject)),
            "directed": lambda n : setattr(self, 'directed', n.get_bool_value()),
            "id": lambda n : setattr(self, 'id', n.get_uuid_value()),
            "sourceItem": lambda n : setattr(self, 'source_item', n.get_object_value(LocatedItemResponse)),
            "targetItem": lambda n : setattr(self, 'target_item', n.get_object_value(LocatedItemResponse)),
            "type": lambda n : setattr(self, 'type', n.get_object_value(RelationshipTypeData)),
            "version": lambda n : setattr(self, 'version', n.get_int_value()),
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
        writer.write_object_value("attributes", self.attributes)
        writer.write_bool_value("directed", self.directed)
        writer.write_uuid_value("id", self.id)
        writer.write_object_value("sourceItem", self.source_item)
        writer.write_object_value("targetItem", self.target_item)
        writer.write_object_value("type", self.type)
        writer.write_int_value("version", self.version)
        writer.write_additional_data_value(self.additional_data)
