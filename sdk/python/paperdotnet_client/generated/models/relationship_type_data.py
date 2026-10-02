from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

@dataclass
class RelationshipTypeData(AdditionalDataHolder, Parsable):
    """
    A taxonomy-backed predicate; constraints apply to directed edges, including recycled endpoints.
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The directed property
    directed: Optional[bool] = None
    # The id property
    id: Optional[UUID] = None
    # The inverseLabel property
    inverse_label: Optional[str] = None
    # The maxIncoming property
    max_incoming: Optional[int] = None
    # The maxOutgoing property
    max_outgoing: Optional[int] = None
    # The name property
    name: Optional[str] = None

    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> RelationshipTypeData:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: RelationshipTypeData
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return RelationshipTypeData()

    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "directed": lambda n : setattr(self, 'directed', n.get_bool_value()),
            "id": lambda n : setattr(self, 'id', n.get_uuid_value()),
            "inverseLabel": lambda n : setattr(self, 'inverse_label', n.get_str_value()),
            "maxIncoming": lambda n : setattr(self, 'max_incoming', n.get_int_value()),
            "maxOutgoing": lambda n : setattr(self, 'max_outgoing', n.get_int_value()),
            "name": lambda n : setattr(self, 'name', n.get_str_value()),
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
        writer.write_str_value("inverseLabel", self.inverse_label)
        writer.write_int_value("maxIncoming", self.max_incoming)
        writer.write_int_value("maxOutgoing", self.max_outgoing)
        writer.write_str_value("name", self.name)
        writer.write_additional_data_value(self.additional_data)
